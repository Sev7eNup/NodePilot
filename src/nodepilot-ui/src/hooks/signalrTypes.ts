// Type definitions and constants shared by `useSignalR` (the React hook that wires up the
// live execution stream) and `signalrReducer` (the pure functions that fold incoming events
// into on-screen state). A separate module so the reducer can be unit-tested without
// pulling in React or @microsoft/signalr.

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
  /** Debugger: "breakpoint" (set by the user) or "stepOver" (from a previous step-over). */
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
// No display cap: every active (Running/Pending) execution renders in the list, and state and
// rendered list are both uncapped. The constant stays as Number.POSITIVE_INFINITY so existing
// imports and tests keep compiling.
export const MAX_ACTIVE_DISPLAYED = Number.POSITIVE_INFINITY;
// On initial mount, auto-hydrate step details only for the most recent active runs; the other
// listing entries show status badges until the user expands them. Exported so tests can assert
// the cap without hardcoding the number.
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
   * Persisted snapshot of the step's OutputParameters dict (redacted at write time).
   * Hydration uses it to rebuild databus entries after a browser refresh, so
   * `{{step.param.X}}` previews keep resolving for terminal runs.
   */
  outputParametersJson?: string | null;
  /**
   * Producing node's `data.outputVariable` alias. Resolved at API time from the workflow
   * definition JSON, since it is not stored on the row, so the rebuilt databus can mirror
   * live entries under both `{stepId}.*` and `{alias}.*` keys.
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
   * Producing node's `data.outputVariable` alias (null when not set). Lets the live databus
   * expose `{alias}.output` and `{alias}.param.*` next to the raw `{stepId}.*` keys, as the
   * engine's BuildStepVariables does.
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
