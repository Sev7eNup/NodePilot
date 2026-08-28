import { useEffect, useMemo, useRef, useState, useCallback, useDeferredValue } from 'react';
import * as signalR from '@microsoft/signalr';
import { useQueryClient } from '@tanstack/react-query';
import { api } from '../api/client';
import { readCsrfToken } from '../api/csrf';
import {
  applyLiveEvents,
  buildDatabusFromHydratedSteps,
  isActiveExecution,
  mergeHydrated,
  mergeListingOnly,
  normalizeBatchItem,
  sortLiveExecutions,
} from './signalrReducer';
import { connectPersistently } from '../lib/signalrConnect';
import {
  AuthBoundaryChangedError,
  assertAuthBoundaryGenerationCurrent,
  captureAuthBoundaryGeneration,
  isAuthBoundaryGenerationCurrent,
} from '../security/authBoundary';
import {
  COMPLETED_EXECUTION_TTL_MS,
  LIVE_EVENT_FLUSH_MS,
  LIVE_REFRESH_INTERVAL_MS,
  MAX_AUTO_HYDRATE,
  type ApiExecutionItem,
  type ApiStepItem,
  type ExecutionUpdate,
  type LiveEvent,
  type LiveEventsBatch,
  type LiveExecutionsById,
  type StepCompletedEvent,
  type StepPausedEvent,
  type StepResumedEvent,
  type StepStartedEvent,
  type StepUpdate,
} from './signalrTypes';

// Re-exported here so callers can keep importing the public surface from this hook
// module directly.
export type { StepUpdate, DatabusEntry, ExecutionUpdate, LiveExecution } from './signalrTypes';
export { COMPLETED_EXECUTION_TTL_MS, MAX_ACTIVE_DISPLAYED, MAX_AUTO_HYDRATE } from './signalrTypes';

/**
 * Reads the double-submit CSRF cookie (non-httpOnly so JS can see it). Attached to the
 * SignalR negotiate handshake via the `headers` option so the server-side CSRF guard
 * accepts the mutating POST even though the request is browser-originated.
 */
// Implementation shared with every other API caller — see api/csrf.ts.

/**
 * Module-scoped semaphore that caps how many step-list backfill fetches the live view
 * can have in flight at once. Prevents a fetch storm when many workflows start at once
 * from saturating the browser's per-origin connection cap and the API server.
 */
const MAX_HYDRATION_CONCURRENCY = 4;
let activeHydrationFetches = 0;
const hydrationQueue: Array<() => void> = [];

function acquireHydrationSlot(): Promise<void> {
  if (activeHydrationFetches < MAX_HYDRATION_CONCURRENCY) {
    activeHydrationFetches++;
    return Promise.resolve();
  }
  return new Promise<void>((resolve) => { hydrationQueue.push(resolve); });
}

function releaseHydrationSlot(): void {
  const next = hydrationQueue.shift();
  // Hand the slot directly to the next waiter without decrementing — keeps the count
  // pinned at MAX while the queue drains, then drops to actual usage when empty.
  if (next) next();
  else activeHydrationFetches--;
}

async function rateLimitedHydration<T>(fetcher: () => Promise<T>): Promise<T> {
  await acquireHydrationSlot();
  try {
    return await fetcher();
  } finally {
    releaseHydrationSlot();
  }
}

export function useWorkflowSignalR(workflowId: string | undefined) {
  const connectionRef = useRef<signalR.HubConnection | null>(null);
  const workflowIdRef = useRef<string | undefined>(workflowId);
  // Desired, not merely confirmed, execution-group memberships. A JoinExecution invocation can be
  // rejected while the DB breaker is open even though the WebSocket stays connected; retaining the
  // intent lets the health recovery transition replay it without user interaction.
  const desiredExecutionGroupsRef = useRef<Set<string>>(new Set());
  const pendingEventsRef = useRef<LiveEvent[]>([]);
  const flushTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const evictionTimersRef = useRef<Map<string, ReturnType<typeof setTimeout>>>(new Map());
  const queryClient = useQueryClient();
  // Debounce token for SignalR-driven query invalidation: coalesces a burst of events
  // into a single refetch per cache key instead of one refetch per event.
  const queryInvalidateTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const pendingInvalidateWorkflowsRef = useRef<Set<string>>(new Set());
  const QUERY_INVALIDATE_DEBOUNCE_MS = 500;

  /**
   * Collects invalidation requests per workflow ID and, after the debounce window,
   * fires one refetch per affected list. A terminal status change is the only kind
   * of event that changes what the list endpoints return.
   */
  const scheduleQueryInvalidate = useCallback((wfId: string) => {
    pendingInvalidateWorkflowsRef.current.add(wfId);
    if (queryInvalidateTimerRef.current !== null) return;
    queryInvalidateTimerRef.current = setTimeout(() => {
      queryInvalidateTimerRef.current = null;
      const wfIds = Array.from(pendingInvalidateWorkflowsRef.current);
      pendingInvalidateWorkflowsRef.current.clear();
      // Per-workflow history lists (the ExecutionPanel history tab + ExecutionsPage filter).
      for (const id of wfIds) {
        queryClient.invalidateQueries({ queryKey: ['workflow-executions', id], exact: false });
      }
      // Global lists that aren't scoped to a workflowId (ExecutionsPage default view, Dashboard).
      queryClient.invalidateQueries({ queryKey: ['executions'], exact: false });
      queryClient.invalidateQueries({ queryKey: ['dashboard-stats'], exact: false });
    }, QUERY_INVALIDATE_DEBOUNCE_MS);
  }, [queryClient]);
  // Tracks executions for which we've fetched full step detail (fully hydrated).
  // Used as a dedup guard: concurrent or repeated calls for the same executionId only
  // hit GET /steps once. Cleared on workflow switch.
  const hydratedExecsRef = useRef<Set<string>>(new Set());
  // Tracks executions shown with listing-only data (status badge, no steps).
  // Prevents the periodic refresh from re-fetching step lists for runs that
  // weren't auto-hydrated on initial load. Cleared on workflow switch.
  const listedExecsRef = useRef<Set<string>>(new Set());
  // Effect lifetime guard shared across callbacks (e.g. lazy hydration triggered from
  // event handlers) so they can also bail out after unmount.
  const mountedRef = useRef<boolean>(true);
  const [liveExecutionsById, setLiveExecutionsById] = useState<LiveExecutionsById>({});
  const [connected, setConnected] = useState(false);

  /** Handles every hub invocation promise at the shared choke point. */
  const invokeSafely = useCallback(async (method: string, ...args: unknown[]): Promise<boolean> => {
    const connection = connectionRef.current;
    if (!connection) return false;
    try {
      await connection.invoke(method, ...args);
      return true;
    } catch (err) {
      console.warn(`[useWorkflowSignalR] ${method} failed`, err);
      return false;
    }
  }, []);

  const liveExecutionsSorted = useMemo(
    () => workflowId
      ? sortLiveExecutions(Object.values(liveExecutionsById).filter((execution) => execution.workflowId === workflowId))
      : [],
    [liveExecutionsById, workflowId],
  );
  // Total active count across the full state — used for the "+N more running" badge.
  // Sourced from the unsorted full list to avoid depending on liveExecutionsSorted's
  // deferred value, which would make the count lag one render behind.
  const liveActiveCount = useMemo(
    () => liveExecutionsSorted.filter(isActiveExecution).length,
    [liveExecutionsSorted],
  );
  // All active runs render — only completed runs are trimmed to the most recent 50 to keep
  // the history tail bounded. State (liveExecutionsById) is also uncapped, so status events
  // reach every run regardless of position.
  const liveExecutionsDisplay = useMemo(() => {
    const active = liveExecutionsSorted.filter(isActiveExecution);
    const done = liveExecutionsSorted.filter((e) => !isActiveExecution(e));
    return [...active, ...done.slice(0, 50)];
  }, [liveExecutionsSorted]);
  // useDeferredValue lets React deprioritise re-sorting the full list when a burst of
  // SignalR events fires state updates in quick succession. User interactions (button
  // clicks, panel resizes) stay responsive while the list catches up asynchronously.
  const liveExecutions = useDeferredValue(liveExecutionsDisplay);
  const liveExecution = liveExecutions[0] ?? null;

  /**
   * Fetches the full step list for one execution and merges it into live state.
   * Idempotent per executionId via `hydratedExecsRef` until the workflow switches.
   * Runs on the first SignalR event for an unseen execution, and again when an
   * execution reaches a terminal status to recover any step events the SignalR
   * channel dropped. On failure the dedup mark is cleared so a later event retries.
   */
  const hydrateStepsForExecution = useCallback(async (executionId: string, execWorkflowId: string) => {
    const authBoundaryGeneration = captureAuthBoundaryGeneration();
    if (!mountedRef.current || workflowIdRef.current !== execWorkflowId) return;
    if (hydratedExecsRef.current.has(executionId)) return;
    hydratedExecsRef.current.add(executionId);
    try {
      const steps = await rateLimitedHydration(
        () => {
          // The hydration limiter may keep this callback queued across logout/user switch.
          assertAuthBoundaryGenerationCurrent(authBoundaryGeneration);
          return api.get<ApiStepItem[]>(`/executions/${executionId}/steps`);
        },
      );
      assertAuthBoundaryGenerationCurrent(authBoundaryGeneration);
      if (!mountedRef.current || workflowIdRef.current !== execWorkflowId) return;
      setLiveExecutionsById((prev) => {
        const exec = prev[executionId];
        const existing = new Map(exec ? exec.steps.map((s) => [s.stepId, s]) : []);
        const mapStatus = (st: string): StepUpdate['status'] =>
          st === 'Cancelled' ? 'Skipped' : (st as StepUpdate['status']);
        const merged: StepUpdate[] = steps.map((s) => {
          const ex = existing.get(s.stepId);
          return {
            executionId,
            workflowId: execWorkflowId,
            stepId: s.stepId,
            stepName: s.stepName ?? ex?.stepName ?? null,
            stepType: s.stepType ?? ex?.stepType ?? 'unknown',
            status: mapStatus(s.status),
            startedAt: s.startedAt ?? ex?.startedAt,
            completedAt: s.completedAt ?? ex?.completedAt,
            output: s.output ?? ex?.output,
            errorOutput: s.errorOutput ?? ex?.errorOutput,
            traceOutput: s.traceOutput ?? ex?.traceOutput,
            traceId: ex?.traceId,
            spanId: ex?.spanId,
          };
        });
        // Preserve in-memory steps not in the REST response (e.g. Paused steps not yet persisted).
        for (const [stepId, step] of existing) {
          if (!merged.some((s) => s.stepId === stepId)) merged.push(step);
        }
        // Rebuild databus entries from the REST snapshot to match the engine's variable
        // shape (`{stepId}.output/.error/.param.*` plus alias-keyed mirrors). Entries
        // already applied from SignalR win on collision since they may be fresher.
        const hydratedDatabus = buildDatabusFromHydratedSteps(steps);
        if (exec) {
          return {
            ...prev,
            [executionId]: {
              ...exec,
              steps: merged,
              databus: { ...hydratedDatabus, ...exec.databus },
            },
          };
        }
        // Lazy path raced ahead of any SignalR event landing in state — synthesize a
        // minimal entry so REST steps are visible immediately. Subsequent SignalR events
        // will fill in workflowId/status correctly via applyLiveEvent's update path.
        const startedAt = merged
          .map((s) => s.startedAt)
          .filter((d): d is string => !!d)
          .sort((a, b) => a.localeCompare(b))[0] ?? new Date().toISOString();
        return {
          ...prev,
          [executionId]: {
            executionId,
            workflowId: execWorkflowId,
            status: 'Running',
            steps: merged,
            startedAt,
            databus: hydratedDatabus,
          },
        };
      });
    } catch (err) {
      if (!(err instanceof AuthBoundaryChangedError)) {
        console.warn(`[useWorkflowSignalR] step hydration for ${executionId} failed`, err);
      }
      hydratedExecsRef.current.delete(executionId);
    }
  }, []);

  /**
   * Schedules deferred removal of an execution from the live state. Idempotent: a
   * second call for the same execution while a timer is pending is a no-op.
   * `delayMs` defaults to the full TTL but accepts a smaller value so callers can
   * account for elapsed time on an already-completed execution. Clamped to 0 so
   * overdue runs still evict on the next tick.
   */
  const scheduleEviction = useCallback((executionId: string, delayMs: number = COMPLETED_EXECUTION_TTL_MS) => {
    if (evictionTimersRef.current.has(executionId)) return;
    const timer = setTimeout(() => {
      evictionTimersRef.current.delete(executionId);
      setLiveExecutionsById((prev) => {
        if (!prev[executionId]) return prev;
        const next = { ...prev };
        delete next[executionId];
        return next;
      });
      // Once evicted from live state the exec is also gone from our hydration tracking —
      // a future event for the same id (rare, but possible if a long-completed run is
      // mentioned again) is allowed to refetch.
      hydratedExecsRef.current.delete(executionId);
    }, Math.max(0, delayMs));
    evictionTimersRef.current.set(executionId, timer);
  }, []);

  /**
   * Bulk refresh of currently-relevant executions for the active workflow. Runs on
   * initial connect, on SignalR auto-reconnect, and periodically as a safety net for
   * runs the SignalR subscription might have missed. `mode='periodic'` only picks up
   * runs not yet known at all, since dropped step events for already-known runs are
   * recovered by the terminal-event handler in the LiveEventsBatch listener. No
   * execution cap here — the server's GET /executions Take(100) is the ceiling.
   * Terminal runs get an eviction timer sized for elapsed time, so a run hydrated
   * after navigating away and back does not stay pinned in the Live tab.
   */
  const hydrateActive = useCallback(async (wfid: string, mode: 'initial' | 'periodic' = 'initial') => {
    const authBoundaryGeneration = captureAuthBoundaryGeneration();
    let all: ApiExecutionItem[];
    try {
      all = await api.get<ApiExecutionItem[]>(`/executions?workflowId=${wfid}&activeOnly=true`);
      assertAuthBoundaryGenerationCurrent(authBoundaryGeneration);
    } catch (err) {
      if (!(err instanceof AuthBoundaryChangedError)) {
        console.warn('[useWorkflowSignalR] bulk hydration: list executions failed', err);
      }
      return;
    }
    const cutoff = Date.now() - COMPLETED_EXECUTION_TTL_MS * 2;
    let relevant = all.filter((e) =>
      e.status === 'Running' ||
      e.status === 'Pending' ||
      (e.completedAt && new Date(e.completedAt).getTime() > cutoff)
    );
    if (mode === 'periodic') {
      // Periodic tick: only pick up runs not yet known at all (neither listed nor hydrated).
      // Terminal-event hydration in the LiveEventsBatch handler covers dropped step events
      // for already-listed runs — periodic stays in its lane.
      relevant = relevant.filter(
        (e) => !hydratedExecsRef.current.has(e.id) && !listedExecsRef.current.has(e.id),
      );
    }
    if (relevant.length === 0) return;
    if (!mountedRef.current || workflowIdRef.current !== wfid) return;

    // Show all relevant runs immediately as status badges (no steps yet).
    // This makes the Live tab populate in one round-trip instead of blocking
    // on N parallel GET /steps fetches.
    setLiveExecutionsById((prev) => mergeListingOnly(relevant, prev));
    for (const exec of relevant) listedExecsRef.current.add(exec.id);

    // Schedule eviction for terminal runs that are already done — there won't be a
    // live ExecutionStatusChanged to trigger it.
    const now = Date.now();
    for (const exec of relevant) {
      if (exec.status === 'Succeeded' || exec.status === 'Failed' || exec.status === 'Cancelled') {
        const elapsed = now - (exec.completedAt ? new Date(exec.completedAt).getTime() : now);
        scheduleEviction(exec.id, COMPLETED_EXECUTION_TTL_MS - elapsed);
      }
    }

    // Auto-hydrate step details for the top MAX_AUTO_HYDRATE most recent active runs.
    // The rest stay as status badges until the user expands them via joinExecution.
    // Both initial and periodic mode apply the same cap, so a burst of newly-discovered
    // runs cannot force every run into full step detail instead of a status badge.
    const toAutoHydrate = relevant
      .filter((e) => e.status === 'Running' || e.status === 'Pending')
      .sort((a, b) => new Date(b.startedAt).getTime() - new Date(a.startedAt).getTime())
      .slice(0, MAX_AUTO_HYDRATE)
      .filter((e) => !hydratedExecsRef.current.has(e.id));

    if (toAutoHydrate.length === 0) return;

    const result: LiveExecutionsById = {};
    await Promise.all(toAutoHydrate.map(async (exec) => {
      try {
        const steps = await rateLimitedHydration(
          () => {
            assertAuthBoundaryGenerationCurrent(authBoundaryGeneration);
            return api.get<ApiStepItem[]>(`/executions/${exec.id}/steps`);
          },
        );
        assertAuthBoundaryGenerationCurrent(authBoundaryGeneration);
        result[exec.id] = {
          executionId: exec.id,
          workflowId: exec.workflowId,
          status: exec.status,
          startedAt: exec.startedAt,
          completedAt: exec.completedAt,
          errorMessage: exec.errorMessage,
          steps: steps.map((s) => ({
            executionId: exec.id,
            workflowId: exec.workflowId,
            stepId: s.stepId,
            stepName: s.stepName,
            stepType: s.stepType ?? 'unknown',
            status: s.status as StepUpdate['status'],
            output: s.output,
            errorOutput: s.errorOutput,
            traceOutput: s.traceOutput,
            startedAt: s.startedAt ?? undefined,
            completedAt: s.completedAt ?? undefined,
          })),
          databus: buildDatabusFromHydratedSteps(steps),
        };
        hydratedExecsRef.current.add(exec.id);
      } catch (err) {
        if (!(err instanceof AuthBoundaryChangedError)) {
          console.warn(`[useWorkflowSignalR] bulk hydration: steps for ${exec.id} failed`, err);
        }
      }
    }));

    if (!mountedRef.current || workflowIdRef.current !== wfid || Object.keys(result).length === 0) return;
    setLiveExecutionsById((prev) => mergeHydrated(result, prev));
  }, [scheduleEviction]);

  const flushPendingEvents = useCallback(() => {
    if (flushTimerRef.current !== null) {
      clearTimeout(flushTimerRef.current);
      flushTimerRef.current = null;
    }

    const events = pendingEventsRef.current;
    if (events.length === 0) return;
    pendingEventsRef.current = [];

    setLiveExecutionsById((prev) => applyLiveEvents(prev, events));
  }, []);

  const enqueueLiveEvent = useCallback((event: LiveEvent) => {
    if (event.evt.workflowId !== workflowIdRef.current) return;

    // Lazy backfill on first step event for a joined execution. Step events (StepStarted/
    // StepCompleted/StepPaused/StepResumed) only arrive after JoinExecution, which already
    // triggers hydrateStepsForExecution; this handles the narrow race where a step event
    // arrives before that fetch completes. ExecutionStatusChanged is excluded because it
    // reaches all workflow-group subscribers, including listing-only runs that must not
    // trigger a step fetch until the user expands them.
    if (
      event.type !== 'ExecutionStatusChanged' &&
      !hydratedExecsRef.current.has(event.evt.executionId)
    ) {
      hydrateStepsForExecution(event.evt.executionId, event.evt.workflowId);
    }

    pendingEventsRef.current.push(event);
    if (event.type === 'ExecutionStatusChanged') {
      const { status, executionId, workflowId: evtWid } = event.evt;
      if (status === 'Succeeded' || status === 'Failed' || status === 'Cancelled') {
        scheduleEviction(executionId);
      }
      // A status change is the only kind of event that actually changes what the list
      // endpoints return (counts, "recent runs" tables). Invalidate the relevant React
      // Query caches here instead of relying on refetchInterval polling — debounced so
      // a burst of events still produces just one refetch per cache key.
      scheduleQueryInvalidate(evtWid);
    }
    if (flushTimerRef.current !== null) return;

    flushTimerRef.current = setTimeout(() => {
      flushTimerRef.current = null;
      flushPendingEvents();
    }, LIVE_EVENT_FLUSH_MS);
  }, [flushPendingEvents, scheduleEviction, hydrateStepsForExecution, scheduleQueryInvalidate]);

  const clearLive = useCallback(() => {
    pendingEventsRef.current = [];
    if (flushTimerRef.current !== null) {
      clearTimeout(flushTimerRef.current);
      flushTimerRef.current = null;
    }
    for (const timer of evictionTimersRef.current.values()) clearTimeout(timer);
    evictionTimersRef.current.clear();
    hydratedExecsRef.current.clear();
    listedExecsRef.current.clear();
    desiredExecutionGroupsRef.current.clear();
    setLiveExecutionsById({});
  }, []);

  /**
   * Joins the per-execution SignalR group and hydrates step details on demand.
   * Called when the user expands a run in the Live tab: the join makes step events
   * start arriving from the backend, and the fetch fills in steps that happened
   * before the join. Idempotent — a call for an already-hydrated execution is a no-op.
   */
  const joinExecution = useCallback(async (executionId: string, execWorkflowId: string) => {
    const authBoundaryGeneration = captureAuthBoundaryGeneration();
    desiredExecutionGroupsRef.current.add(executionId);
    if (!await invokeSafely('JoinExecution', executionId)) return;
    if (!mountedRef.current
      || !isAuthBoundaryGenerationCurrent(authBoundaryGeneration)) return;
    // Explicit expansion refetches steps that started outside the execution-specific group.
    // Clearing the dedup mark forces a fresh fetch that covers the gap.
    hydratedExecsRef.current.delete(executionId);
    await hydrateStepsForExecution(executionId, execWorkflowId);
  }, [hydrateStepsForExecution, invokeSafely]);

  /** Leaves the per-execution SignalR group when the user collapses a run. */
  const leaveExecution = useCallback(async (executionId: string) => {
    desiredExecutionGroupsRef.current.delete(executionId);
    await invokeSafely('LeaveExecution', executionId);
  }, [invokeSafely]);

  /**
   * Restores all desired subscriptions after either a transport reconnect or a database-only
   * recovery. Group joins are idempotent server-side. Hydrating desired executions closes the gap
   * for joins that failed while the hub filter was returning DATABASE_UNAVAILABLE.
   */
  const reconcileSubscriptions = useCallback(async (wfid: string) => {
    const authBoundaryGeneration = captureAuthBoundaryGeneration();
    if (!mountedRef.current || workflowIdRef.current !== wfid) return;

    await invokeSafely('JoinWorkflow', wfid);
    if (!mountedRef.current
      || !isAuthBoundaryGenerationCurrent(authBoundaryGeneration)) return;
    const desired = Array.from(desiredExecutionGroupsRef.current);
    await Promise.all(desired.map(async (executionId) => {
      if (await invokeSafely('JoinExecution', executionId)
        && mountedRef.current
        && isAuthBoundaryGenerationCurrent(authBoundaryGeneration)) {
        await hydrateStepsForExecution(executionId, wfid);
      }
    }));

    if (!mountedRef.current
      || workflowIdRef.current !== wfid
      || !isAuthBoundaryGenerationCurrent(authBoundaryGeneration)) return;
    await hydrateActive(wfid);
    scheduleQueryInvalidate(wfid);
  }, [hydrateActive, hydrateStepsForExecution, invokeSafely, scheduleQueryInvalidate]);

  useEffect(() => {
    workflowIdRef.current = workflowId;
    clearLive();
  }, [clearLive, workflowId]);

  useEffect(() => {
    if (!workflowId) return;
    mountedRef.current = true;

    // The httpOnly np_auth cookie travels on the negotiate POST and the WebSocket upgrade
    // automatically (same-origin, `withCredentials` default for SignalR browser transport).
    const connection = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/execution', {
        headers: { 'X-CSRF-Token': readCsrfToken() },
      })
      .withAutomaticReconnect()
      .configureLogging(signalR.LogLevel.Warning)
      .build();

    connectionRef.current = connection;

    connection.on('StepStarted', (evt: StepStartedEvent) => enqueueLiveEvent({ type: 'StepStarted', evt }));
    connection.on('StepCompleted', (evt: StepCompletedEvent) => enqueueLiveEvent({ type: 'StepCompleted', evt }));
    connection.on('ExecutionStatusChanged', (evt: ExecutionUpdate) => enqueueLiveEvent({ type: 'ExecutionStatusChanged', evt }));
    connection.on('StepPaused', (evt: StepPausedEvent) => enqueueLiveEvent({ type: 'StepPaused', evt }));
    connection.on('StepResumed', (evt: StepResumedEvent) => enqueueLiveEvent({ type: 'StepResumed', evt }));
    connection.on('LiveEventsBatch', (batch: LiveEventsBatch) => {
      const items = Array.isArray(batch) ? batch : (batch.events ?? batch.Events ?? []);
      for (const item of items) {
        const event = normalizeBatchItem(item);
        if (!event) continue;
        enqueueLiveEvent(event);
        // Terminal-state safety refetch: when an execution finishes, force a re-pull of
        // its step list to fill in any StepCompleted events the bounded SignalR channel
        // dropped under burst load. Reset the dedup mark so this call goes through even
        // if lazy-hydration already ran earlier in the run's lifetime.
        if (event.type === 'ExecutionStatusChanged') {
          const { status, executionId, workflowId: evtWid } = event.evt;
          if (status === 'Succeeded' || status === 'Failed' || status === 'Cancelled') {
            // Re-fetch only an execution that the user fully hydrated by joining or expanding it.
            // This recovers StepCompleted events dropped by
            // the bounded SignalR channel under burst load. Listing-only runs (never joined)
            // had no step events to drop, so a re-fetch would be pure waste.
            if (hydratedExecsRef.current.has(executionId)) {
              hydratedExecsRef.current.delete(executionId);
              hydrateStepsForExecution(executionId, evtWid);
            }
          }
        }
      }
    });

    // One body for both fresh connects and automatic reconnects, run by connectPersistently.
    // After a full close + start(), onreconnected never fires and hub group memberships are
    // gone, so the re-join, hydration, and cache invalidation must all happen on every kind
    // of (re)connect, and the connection indicator must update on every one of them too.
    const persistent = connectPersistently(
      connection,
      async () => {
        if (!mountedRef.current) return;
        setConnected(true);
        await reconcileSubscriptions(workflowId);
      },
      () => {
        // The automatic-reconnect policy gave up, so the indicator must stop claiming
        // the app is connected.
        if (mountedRef.current) setConnected(false);
      },
      // The WebSocket may survive a DB outage. Replay groups on unavailable -> ok even without a
      // transport lifecycle event; connectPersistently calls this only while state === Connected.
      () => reconcileSubscriptions(workflowId));

    // Periodic refresh: catches new executions whose initial SignalR event we missed
    // (rare — the Hub is reliable, but JoinWorkflow timing or a transient drop could
    // skip one). Hydrated execs are skipped — terminal-event hydration in the
    // LiveEventsBatch handler covers their dropped step events.
    const refreshTimer = setInterval(() => {
      if (!mountedRef.current) return;
      hydrateActive(workflowId, 'periodic');
    }, LIVE_REFRESH_INTERVAL_MS);

    return () => {
      mountedRef.current = false;
      clearInterval(refreshTimer);
      pendingEventsRef.current = [];
      if (flushTimerRef.current !== null) {
        clearTimeout(flushTimerRef.current);
        flushTimerRef.current = null;
      }
      if (queryInvalidateTimerRef.current !== null) {
        clearTimeout(queryInvalidateTimerRef.current);
        queryInvalidateTimerRef.current = null;
        pendingInvalidateWorkflowsRef.current.clear();
      }
      for (const timer of evictionTimersRef.current.values()) clearTimeout(timer);
      evictionTimersRef.current.clear();
      persistent.dispose();
      connection.stop();
      setConnected(false);
    };
  }, [enqueueLiveEvent, workflowId, hydrateActive, hydrateStepsForExecution, reconcileSubscriptions, scheduleQueryInvalidate]);

  return { liveExecution, liveExecutions, liveActiveCount, connected, clearLive, joinExecution, leaveExecution };
}
