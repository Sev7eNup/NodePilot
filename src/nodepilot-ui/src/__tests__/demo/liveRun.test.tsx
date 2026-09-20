/**
 * The fake hub and the run player.
 *
 * Two properties are load-bearing and easy to get wrong:
 *
 * 1. Group routing. The workflow group carries only `ExecutionStatusChanged`; step events
 *    reach a client only after `JoinExecution`; the operations feed carries only
 *    `LiveEventsBatch`. Getting this wrong leaves the canvas either dead or too chatty.
 * 2. Write-then-emit ordering. The operations feed invalidates query keys on every event, and
 *    the refetch hits the demo's own REST layer. Emitting first would serve a state older
 *    than the event that caused the refetch.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { HubConnection } from '@microsoft/signalr';
import { createElement, type ReactNode } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook, waitFor } from '@testing-library/react';
import { useLiveOpsFeed } from '../../hooks/useLiveOpsFeed';
import { setExecutionHubFactory } from '../../lib/hubConnection';
import { applyLiveEvents } from '../../hooks/signalrReducer';
import type { LiveEvent, LiveExecutionsById } from '../../hooks/signalrTypes';
import {
  createFakeHubConnection,
  emitExecutionStatus,
  emitStepEvent,
  fakeHubConnectionCount,
  resetFakeHub,
} from '../../../demo/hub/fakeHub';
import { activeRunIds, cancelRun, startRun, stopAllRuns } from '../../../demo/run/player';
import { configureWorld, getWorld, resetWorld } from '../../../demo/state/world';
import { buildWorld } from '../../../demo/seed/build';

const NOW = Date.parse('2026-09-18T12:00:00.000Z');

type Recorded = { name: string; payload: unknown };

async function connect(): Promise<{ connection: HubConnection; received: Recorded[] }> {
  const connection = createFakeHubConnection();
  const received: Recorded[] = [];
  for (const name of ['StepStarted', 'StepCompleted', 'ExecutionStatusChanged', 'StepPaused', 'StepResumed', 'LiveEventsBatch']) {
    connection.on(name, (payload: unknown) => received.push({ name, payload }));
  }
  await connection.start();
  return { connection, received };
}

describe('fake hub group routing', () => {
  afterEach(() => { resetFakeHub(); });

  it('satisfies the lifecycle contract connectPersistently probes for', async () => {
    const connection = createFakeHubConnection() as unknown as Record<string, unknown>;
    // signalrConnect guards each hook with `typeof connection.X === 'function'` and treats an
    // undefined state as down, so all four have to be present.
    expect(typeof connection.onclose).toBe('function');
    expect(typeof connection.onreconnecting).toBe('function');
    expect(typeof connection.onreconnected).toBe('function');
    expect(connection.state).toBe('Disconnected');
    await (connection.start as () => Promise<void>)();
    expect(connection.state).toBe('Connected');
  });

  it('sends the workflow group execution status only', async () => {
    const { connection, received } = await connect();
    await connection.invoke('JoinWorkflow', 'wf-1');

    emitExecutionStatus({ executionId: 'ex-1', workflowId: 'wf-1', status: 'Running' });
    emitStepEvent({
      name: 'StepStarted',
      event: { executionId: 'ex-1', workflowId: 'wf-1', stepId: 'n1', stepType: 'log', startedAt: '2026-09-18T12:00:00Z' },
    });

    expect(received.map((r) => r.name)).toEqual(['ExecutionStatusChanged']);
  });

  it('delivers step events only after JoinExecution, and stops after LeaveExecution', async () => {
    const { connection, received } = await connect();
    const step = { executionId: 'ex-1', workflowId: 'wf-1', stepId: 'n1', stepType: 'log', startedAt: '2026-09-18T12:00:00Z' };

    emitStepEvent({ name: 'StepStarted', event: step });
    expect(received).toHaveLength(0);

    await connection.invoke('JoinExecution', 'ex-1');
    emitStepEvent({ name: 'StepStarted', event: step });
    expect(received.map((r) => r.name)).toEqual(['StepStarted']);

    await connection.invoke('LeaveExecution', 'ex-1');
    emitStepEvent({ name: 'StepStarted', event: step });
    expect(received).toHaveLength(1);
  });

  it('sends the operations feed batches only', async () => {
    const { connection, received } = await connect();
    await connection.invoke('JoinOperationsFeed');

    emitExecutionStatus({ executionId: 'ex-1', workflowId: 'wf-1', status: 'Succeeded' });
    emitStepEvent({
      name: 'StepStarted',
      event: { executionId: 'ex-1', workflowId: 'wf-1', stepId: 'n1', stepType: 'log', startedAt: '2026-09-18T12:00:00Z' },
    });

    expect(received.map((r) => r.name)).toEqual(['LiveEventsBatch']);
    expect(received[0].payload).toMatchObject({ events: [{ type: 'ExecutionStatusChanged' }] });
  });

  it('drops a connection from every group on stop', async () => {
    const { connection } = await connect();
    expect(fakeHubConnectionCount()).toBe(1);
    await connection.stop();
    expect(fakeHubConnectionCount()).toBe(0);
  });
});

describe('run player', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    configureWorld(() => buildWorld(NOW));
  });

  afterEach(() => {
    stopAllRuns();
    vi.useRealTimers();
    resetFakeHub();
    resetWorld();
  });

  /** Replaces a workflow's graph, the way a visitor editing the canvas would. */
  function setGraph(workflowId: string, nodes: unknown[], edges: unknown[]): void {
    const workflow = getWorld().workflows.find((w) => w.id === workflowId)!;
    workflow.definitionJson = JSON.stringify({ nodes, edges });
  }

  it('walks the graph currently on the canvas, not a recorded script', async () => {
    const workflowId = getWorld().workflows[0].id;
    // Node ids a seed script could never know about.
    setGraph(
      workflowId,
      [
        { id: 'freshly-added-trigger', data: { activityType: 'manualTrigger', label: 'Start' } },
        { id: 'freshly-added-step', data: { activityType: 'runScript', label: 'Do work' } },
      ],
      [{ id: 'e1', source: 'freshly-added-trigger', target: 'freshly-added-step' }],
    );

    const { connection, received } = await connect();
    const execution = startRun(workflowId)!;
    await connection.invoke('JoinExecution', execution.id);

    await vi.advanceTimersByTimeAsync(10_000);

    const started = received.filter((r) => r.name === 'StepStarted').map((r) => (r.payload as { stepId: string }).stepId);
    expect(started).toEqual(['freshly-added-trigger', 'freshly-added-step']);
  });

  it('writes each step into the world before delivering its event', async () => {
    const workflowId = getWorld().workflows[0].id;
    setGraph(
      workflowId,
      [{ id: 'n1', data: { activityType: 'manualTrigger', label: 'Start' } }],
      [],
    );

    const connection = createFakeHubConnection();
    const seenWhileHandling: number[] = [];
    let executionId = '';
    connection.on('StepStarted', () => {
      // The world must already carry the step when the event arrives; a refetch triggered by
      // this event has to see at least as much as the event reports.
      seenWhileHandling.push((getWorld().steps.get(executionId) ?? []).length);
    });
    await connection.start();

    const execution = startRun(workflowId)!;
    executionId = execution.id;
    await connection.invoke('JoinExecution', executionId);
    await vi.advanceTimersByTimeAsync(10_000);

    expect(seenWhileHandling).toEqual([1]);
  });

  it('finishes a run and re-derives the workflow counters from the history', async () => {
    const workflowId = getWorld().workflows[0].id;
    const before = getWorld().workflows[0].totalCount ?? 0;
    setGraph(workflowId, [{ id: 'n1', data: { activityType: 'manualTrigger' } }], []);

    startRun(workflowId);
    await vi.advanceTimersByTimeAsync(10_000);

    const workflow = getWorld().workflows.find((w) => w.id === workflowId)!;
    const runs = getWorld().executions.filter((e) => e.workflowId === workflowId);
    expect(workflow.totalCount).toBe(before + 1);
    expect(workflow.totalCount).toBe(runs.length);
    expect(runs[0].status).toBe('Succeeded');
    expect(activeRunIds()).toEqual([]);
  });

  it('fails a run whose graph has no enabled trigger, like the engine does', async () => {
    const workflowId = getWorld().workflows[0].id;
    setGraph(workflowId, [{ id: 'n1', data: { activityType: 'runScript' } }], []);

    const execution = startRun(workflowId)!;
    await vi.advanceTimersByTimeAsync(5_000);

    const finished = getWorld().executions.find((e) => e.id === execution.id)!;
    expect(finished.status).toBe('Failed');
    expect(finished.errorMessage).toContain('trigger');
  });

  it('cancels a run and leaves the in-flight step cancelled', async () => {
    const workflowId = getWorld().workflows[0].id;
    setGraph(
      workflowId,
      [
        { id: 'n1', data: { activityType: 'manualTrigger' } },
        { id: 'n2', data: { activityType: 'runScript' } },
      ],
      [{ id: 'e1', source: 'n1', target: 'n2' }],
    );

    const execution = startRun(workflowId)!;
    await vi.advanceTimersByTimeAsync(400);
    expect(cancelRun(execution.id)).toBe(true);

    const finished = getWorld().executions.find((e) => e.id === execution.id)!;
    expect(finished.status).toBe('Cancelled');
    expect(getWorld().steps.get(execution.id)?.some((s) => s.status === 'Cancelled')).toBe(true);
  });

  it('produces events the live reducer folds into a finished execution', async () => {
    const workflowId = getWorld().workflows[0].id;
    setGraph(
      workflowId,
      [
        { id: 'n1', data: { activityType: 'manualTrigger', label: 'Start' } },
        { id: 'n2', data: { activityType: 'log', label: 'Write log' } },
      ],
      [{ id: 'e1', source: 'n1', target: 'n2' }],
    );

    const connection = createFakeHubConnection();
    const events: LiveEvent[] = [];
    connection.on('StepStarted', (evt: unknown) => events.push({ type: 'StepStarted', evt } as LiveEvent));
    connection.on('StepCompleted', (evt: unknown) => events.push({ type: 'StepCompleted', evt } as LiveEvent));
    connection.on('ExecutionStatusChanged', (evt: unknown) => events.push({ type: 'ExecutionStatusChanged', evt } as LiveEvent));
    await connection.start();

    const execution = startRun(workflowId)!;
    await connection.invoke('JoinWorkflow', workflowId);
    await connection.invoke('JoinExecution', execution.id);
    await vi.advanceTimersByTimeAsync(10_000);

    // The demo feeds the product's own reducer, so the canvas state it drives is the real one.
    const state = applyLiveEvents({} as LiveExecutionsById, events);
    const live = state[execution.id];
    expect(live.status).toBe('Succeeded');
    expect(live.steps.map((s) => s.stepId)).toEqual(['n1', 'n2']);
    expect(live.steps.every((s) => s.status === 'Succeeded')).toBe(true);
  });
});

/**
 * The operations feed, driven through the real hook.
 *
 * This is the test that was missing. The earlier one asserted the batch object the fake hub
 * produced, which is circular: the hub emitted `{type, event}` and the assertion checked for
 * `{type, event}`, while the consumer reads `Event ?? evt` and saw nothing. Dashboard and
 * Live-Ops fell back to polling and no test noticed.
 */
describe('operations feed, through the real hook', () => {
  beforeEach(() => {
    configureWorld(() => buildWorld(NOW));
    setExecutionHubFactory(createFakeHubConnection);
  });

  afterEach(() => {
    setExecutionHubFactory();
    resetFakeHub();
    resetWorld();
  });

  it('delivers an execution status change to the feed consumer', async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const invalidated: unknown[] = [];
    queryClient.invalidateQueries = ((filters?: { queryKey?: unknown }) => {
      invalidated.push(filters?.queryKey);
      return Promise.resolve();
    }) as typeof queryClient.invalidateQueries;

    const seen: { executionId: string; workflowId: string; status: string }[] = [];
    const onStatus = (executionId: string, workflowId: string, status: string) =>
      seen.push({ executionId, workflowId, status });
    const queryKey = ['operations-graph'];

    const wrapper = ({ children }: { children: ReactNode }) =>
      createElement(QueryClientProvider, { client: queryClient }, children);

    renderHook(() => useLiveOpsFeed({ queryKey, debounceMs: 1, onStatus }), { wrapper });

    // The hook joins the feed once the connection starts; wait for that before emitting.
    await waitFor(() => expect(fakeHubConnectionCount()).toBe(1));
    await act(async () => {
      emitExecutionStatus({ executionId: 'ex-1', workflowId: 'wf-1', status: 'Succeeded' });
    });

    await waitFor(() => expect(seen).toHaveLength(1));
    expect(seen[0]).toEqual({ executionId: 'ex-1', workflowId: 'wf-1', status: 'Succeeded' });
    await waitFor(() => expect(invalidated).toContainEqual(queryKey));
  });
});
