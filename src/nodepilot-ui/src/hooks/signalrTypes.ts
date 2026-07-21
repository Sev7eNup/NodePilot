// Type definitions and constants shared by `useSignalR` (the React hook that wires up
// the live execution stream) and `signalrReducer` (the pure functions that fold incoming
// events into the on-screen state). Kept as a separate module so the reducer can be
// unit-tested without pulling in React or @microsoft/signalr.

export interface StepUpdate {
  executionId: string;
  workflowId: string;
  stepId: string;
  stepName?: string | null;
  stepType: string;
  status: 'Running' | 'Succeeded' | 'Failed' | 'Skipped' | 'Paused';
  output?: string | null;
  errorOutput?: string | null;
  /** PowerShell Start-Transcript capture (RunScript with `config.transcript:true`). */
  traceOutput?: string | null;
  startedAt?: string;
  completedAt?: string;
  traceId?: string | null;
  spanId?: string | null;
  /** Debugger: timestamp of the last pause. Set when status=Paused. */
  pausedAt?: string;
  /** Debugger: snapshot of the variables at the moment of the pause (secrets already redacted). */
  pausedVariables?: Record<string, string>;
  /** Debugger: "breakpoint" (set by the user) or "stepOver" (triggered by a previous step-over). */
  pausedReason?: string;
}

export interface DatabusEntry {
  value: string;
  stepId?: string;
  stepName?: string | null;
  kind: 'output' | 'error' | 'param' | 'trigger' | 'global' | 'other';
  paramKey?: string;
}

export interface ExecutionUpdate {
  executionId: string;
  workflowId: string;
  status: string;
  errorMessage?: string | null;
  completedAt?: string | null;
  traceId?: string | null;
}

export interface LiveExecution {
  executionId: string;
  workflowId?: string;
  status: string;
  steps: StepUpdate[];
  startedAt: string;
  completedAt?: string | null;
  errorMessage?: string | null;
  databus: Record<string, DatabusEntry>;
}

export type LiveExecutionsById = Record<string, LiveExecution>;

export const LIVE_EVENT_FLUSH_MS = 100;
export const COMPLETED_EXECUTION_TTL_MS = 30_000;
export const LIVE_REFRESH_INTERVAL_MS = 10_000;
// All active (Running/Pending) executions render in the list — no display cap. State and
// rendered list are both uncapped. The legacy "+N more"-badge threshold is gone; this
// constant is retained as Number.POSITIVE_INFINITY only so existing imports/tests compile.
export const MAX_ACTIVE_DISPLAYED = Number.POSITIVE_INFINITY;
// On initial mount only auto-hydrate step details for the N most recent active runs.
// The remaining listing entries show status badges only until the user expands them.
// Exported so tests can assert the cap without hardcoding the magic number.
export const MAX_AUTO_HYDRATE = 10;

export type ApiExecutionItem = {
  id: string;
  workflowId: string;
  status: string;
  startedAt: string;
  completedAt?: string | null;
  errorMessage?: string | null;
};

export type ApiStepItem = {
  stepId: string;
  stepName?: string | null;
  stepType?: string | null;
  status: string;
  startedAt?: string | null;
  completedAt?: string | null;
  output?: string | null;
  errorOutput?: string | null;
  traceOutput?: string | null;
  /**
   * Persisted snapshot of the step's OutputParameters dict (already redacted at write).
   * Used by hydration to rebuild databus entries after a browser refresh — without this,
   * `{{step.param.X}}` previews would go blank for terminal runs even though the data is
   * available server-side.
   */
  outputParametersJson?: string | null;
  /**
   * Producing node's `data.outputVariable` alias. Resolved at API time from the workflow
   * definition JSON (not stored on the row) so the rebuilt databus can mirror live entries
   * under both `{stepId}.*` and `{alias}.*` keys.
   */
  outputVariable?: string | null;
};

export type StepStartedEvent = {
  executionId: string;
  workflowId: string;
  stepId: string;
  stepName?: string;
  stepType: string;
  startedAt: string;
  traceId?: string | null;
  spanId?: string | null;
};

export type StepCompletedEvent = {
  executionId: string;
  workflowId: string;
  stepId: string;
  stepName?: string | null;
  status: string;
  output?: string;
  errorOutput?: string;
  completedAt: string;
  traceId?: string | null;
  spanId?: string | null;
  outputParameters?: Record<string, string> | null;
  traceOutput?: string | null;
  stepType?: string | null;
  startedAt?: string | null;
  /**
   * Producing node's `data.outputVariable` alias (null when not set). Allows the live
   * databus to expose `{alias}.output` and `{alias}.param.*` alongside the raw
   * `{stepId}.*` keys, matching the engine's BuildStepVariables dual-lookup contract.
   */
  outputVariable?: string | null;
};

export type StepPausedEvent = {
  executionId: string;
  workflowId: string;
  stepId: string;
  stepName?: string;
  variables: Record<string, string>;
  pausedAt: string;
  reason: string;
};

export type StepResumedEvent = {
  executionId: string;
  workflowId: string;
  stepId: string;
};

export type LiveEvent =
  | { type: 'StepStarted'; evt: StepStartedEvent }
  | { type: 'StepCompleted'; evt: StepCompletedEvent }
  | { type: 'ExecutionStatusChanged'; evt: ExecutionUpdate }
  | { type: 'StepPaused'; evt: StepPausedEvent }
  | { type: 'StepResumed'; evt: StepResumedEvent };

export type LiveEventBatchItem = {
  type?: LiveEvent['type'];
  Type?: LiveEvent['type'];
  event?: StepStartedEvent | StepCompletedEvent | ExecutionUpdate | StepPausedEvent | StepResumedEvent;
  Event?: StepStartedEvent | StepCompletedEvent | ExecutionUpdate | StepPausedEvent | StepResumedEvent;
  evt?: StepStartedEvent | StepCompletedEvent | ExecutionUpdate | StepPausedEvent | StepResumedEvent;
};

export type LiveEventsBatch = { events?: LiveEventBatchItem[]; Events?: LiveEventBatchItem[] } | LiveEventBatchItem[];
