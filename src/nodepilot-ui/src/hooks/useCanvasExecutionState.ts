import { useState, useEffect, useMemo, useCallback } from 'react';
import { useQuery } from '@tanstack/react-query';
import { api } from '../api/client';
import type { StepExecution } from '../types/api';
import type { LiveExecution } from './useSignalR';

interface UseCanvasExecutionStateArgs {
  /** Live executions from useWorkflowSignalR — used to resolve the pinned canvas run. */
  liveExecutions: LiveExecution[];
  /** Current workflow id (route param). Resets the pinned/replay state on change. */
  workflowId: string | undefined;
  joinExecution: (executionId: string, workflowId: string) => Promise<void>;
  leaveExecution: (executionId: string) => Promise<void>;
}

/**
 * Replay/snapshot state machine for the designer canvas. Owns, as one cohesive unit:
 *  - the *pinned* live execution whose green path-coloring is shown on the canvas
 *    (`designerCanvasExecutionId`), incl. a snapshot that outlives the 30 s SignalR TTL
 *    eviction so the highlight + test-run banner stay until the user dismisses them,
 *  - the per-execution SignalR group join/leave for that pinned run, and
 *  - the *replay* timeline (a terminal execution scrubbed via `scrubTimeMs`).
 *
 * Exposes a narrow, intent-oriented command surface (pin / toggleReplay / scrub /
 * clearReplay / clearDesignerCanvasHighlight) — no raw setters leak out.
 */
export function useCanvasExecutionState({ liveExecutions, workflowId, joinExecution, leaveExecution }: UseCanvasExecutionStateArgs) {
  const [designerCanvasExecutionId, setDesignerCanvasExecutionId] = useState<string | null>(null);
  const [canvasRunIsTerminalState, setCanvasRunIsTerminalState] = useState(false);
  // Snapshot of the canvas execution so the green path-coloring + test-run banner
  // survive the 30s TTL eviction in useSignalR. Without this the canvas reverts to its
  // pristine state the moment liveExecutionsById drops the run, even though the user
  // hasn't dismissed the highlight via the X-button yet.
  const [canvasExecutionSnapshot, setCanvasExecutionSnapshot] = useState<LiveExecution | null>(null);
  const [replayExecutionId, setReplayExecutionId] = useState<string | null>(null);
  const [scrubTimeMs, setScrubTimeMs] = useState<number | null>(null);

  const { data: replaySteps } = useQuery({
    queryKey: ['replay-steps', replayExecutionId],
    queryFn: () => api.get<StepExecution[]>(`/executions/${replayExecutionId}/steps`),
    enabled: !!replayExecutionId,
    staleTime: Infinity,
  });

  const clearReplay = useCallback(() => { setReplayExecutionId(null); setScrubTimeMs(null); }, []);
  const clearDesignerCanvasHighlight = useCallback(() => {
    setDesignerCanvasExecutionId(null);
    setCanvasRunIsTerminalState(false);
    setCanvasExecutionSnapshot(null);
  }, []);

  const canvasLiveExecution = useMemo(
    () => designerCanvasExecutionId
      ? liveExecutions.find((execution) => execution.executionId === designerCanvasExecutionId) ?? null
      : null,
    [designerCanvasExecutionId, liveExecutions],
  );

  // Keep a fresh snapshot of the canvas execution while it's still in the SignalR state,
  // and surface the terminal-state flag in the same pass. Previously these were two
  // useEffects (one on canvasLiveExecution, one on effectiveCanvasExecution) — under a
  // SignalR burst that fired the same logical state change through both paths, the
  // snapshot-update and the terminal-flag-update could interleave across renders.
  // After the 30 s TTL drops the live entry, canvasLiveExecution goes null and we
  // fall back to canvasExecutionSnapshot (computed below) so the green path-coloring
  // and the test-run banner stay until the user dismisses them via the X-button.
  useEffect(() => {
    if (!canvasLiveExecution) return;
    setCanvasExecutionSnapshot(canvasLiveExecution);
    if (
      canvasLiveExecution.steps.length > 0
      && ['Succeeded', 'Failed', 'Cancelled'].includes(canvasLiveExecution.status)
    ) {
      setCanvasRunIsTerminalState(true);
    }
  }, [canvasLiveExecution]);
  const effectiveCanvasExecution = canvasLiveExecution ?? canvasExecutionSnapshot;

  // Auto-join the per-execution SignalR group as soon as a canvas execution is set,
  // so StepStarted/StepCompleted events flow into liveExecution.steps and the canvas
  // pulse (an amber "Running" animation) lights up immediately. Without this the
  // editor only sees ExecutionStatusChanged on the workflow firehose — step-level
  // events were moved off that channel for the 200-job parallel-run perf fix
  // (commit 1e6b42f), so the canvas would otherwise stay un-pulsing until the
  // 10-second hydrateActive tick backfills steps via REST.
  useEffect(() => {
    if (!designerCanvasExecutionId) return;
    void joinExecution(designerCanvasExecutionId, workflowId ?? '');
    return () => { void leaveExecution(designerCanvasExecutionId); };
  }, [designerCanvasExecutionId, workflowId, joinExecution, leaveExecution]);

  const designerCanvasRunIsTerminal = !!designerCanvasExecutionId && canvasRunIsTerminalState;
  const designerCanvasRunShortId = designerCanvasExecutionId?.slice(0, 8) ?? '';

  // Reset pinned + snapshot state when switching workflows.
  useEffect(() => {
    setDesignerCanvasExecutionId(null);
    setCanvasRunIsTerminalState(false);
    setCanvasExecutionSnapshot(null);
  }, [workflowId]);

  /** Pin a live execution's path-coloring onto the canvas (e.g. when a run starts). */
  const pinCanvasExecution = useCallback((executionId: string) => {
    setDesignerCanvasExecutionId(executionId);
  }, []);
  /** Toggle scrubbable replay for a terminal execution; clears any live canvas highlight first. */
  const toggleReplay = useCallback((executionId: string) => {
    clearDesignerCanvasHighlight();
    setReplayExecutionId((prev) => prev === executionId ? null : executionId);
  }, [clearDesignerCanvasHighlight]);
  /** Move the replay scrubber (null = no scrub). */
  const scrubTo = useCallback((t: number | null) => { setScrubTimeMs(t); }, []);

  return {
    effectiveCanvasExecution,
    replayExecutionId,
    replaySteps,
    scrubTimeMs,
    designerCanvasRunIsTerminal,
    designerCanvasRunShortId,
    pinCanvasExecution,
    clearReplay,
    clearDesignerCanvasHighlight,
    toggleReplay,
    scrubTo,
  };
}
