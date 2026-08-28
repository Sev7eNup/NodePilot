import { useState, useEffect, useMemo, useCallback } from 'react';
import { useQuery } from '@tanstack/react-query';
import { api } from '../api/client';
import type { StepExecution } from '../types/api';
import type { LiveExecution } from './useSignalR';

interface UseCanvasExecutionStateArgs {
/** Live executions from useWorkflowSignalR for resolving the pinned canvas run. */
  liveExecutions: LiveExecution[];
  /** Current workflow id (route param). Resets the pinned/replay state on change. */
  workflowId: string | undefined;
  joinExecution: (executionId: string, workflowId: string) => Promise<void>;
  leaveExecution: (executionId: string) => Promise<void>;
}

/**
 * Replay and snapshot state machine for the designer canvas. It owns three things:
 *  - the pinned live execution whose path coloring is shown on the canvas
 *    (`designerCanvasExecutionId`), plus a snapshot that outlives the SignalR TTL eviction so
 *    highlight and test-run banner stay until the user dismisses them,
 *  - the per-execution SignalR group join and leave for that pinned run, and
 *  - the replay timeline (a terminal execution scrubbed via `scrubTimeMs`).
 *
 * Only intent-oriented commands are exposed (pin, toggleReplay, scrub, clearReplay,
 * clearDesignerCanvasHighlight); no raw setters leak out.
 */
export function useCanvasExecutionState({ liveExecutions, workflowId, joinExecution, leaveExecution }: UseCanvasExecutionStateArgs) {
  const [designerCanvasExecutionId, setDesignerCanvasExecutionId] = useState<string | null>(null);
  const [canvasRunIsTerminalState, setCanvasRunIsTerminalState] = useState(false);
  // Snapshot of the canvas execution so path coloring and test-run banner survive the TTL
  // eviction in useSignalR. Without it the canvas resets the moment liveExecutionsById drops
  // the run, before the user has dismissed the highlight.
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

  // Keep a fresh snapshot of the canvas execution while it is still in the SignalR state and
  // set the terminal-state flag in the same pass, so a burst of events cannot interleave the
  // two updates across renders. Once the TTL drops the live entry, canvasLiveExecution goes
  // null and canvasExecutionSnapshot (used below) takes over, keeping the path coloring and
  // the test-run banner on screen until the user dismisses them.
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

  // Join the per-execution SignalR group as soon as a canvas execution is set, so
  // StepStarted/StepCompleted events flow into liveExecution.steps and the canvas pulse (an
  // amber "Running" animation) starts at once. The workflow-wide firehose carries only
  // ExecutionStatusChanged, so without this the canvas would stay static until the
  // hydrateActive tick backfills steps via REST.
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

  /** Pin a live execution's path coloring onto the canvas, for example when a run starts. */
  const pinCanvasExecution = useCallback((executionId: string) => {
    setDesignerCanvasExecutionId(executionId);
  }, []);
  /** Toggle scrubbable replay for a terminal execution; clears any live canvas highlight first. */
  const toggleReplay = useCallback((executionId: string) => {
    clearDesignerCanvasHighlight();
    setReplayExecutionId((prev) => prev === executionId ? null : executionId);
  }, [clearDesignerCanvasHighlight]);
  /** Move the replay scrubber; null means no scrub. */
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
