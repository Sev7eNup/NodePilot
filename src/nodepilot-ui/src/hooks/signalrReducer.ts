// Pure reducer functions for the live execution stream. No React, no SignalR client,
// no DOM — just `prev state + event(s) → next state`. Extracted from `useSignalR` so the
// merging / sorting / classification logic can be unit-tested in isolation and the hook
// stays focused on connection lifecycle + side effects.

import type {
  ApiExecutionItem,
  DatabusEntry,
  LiveEvent,
  LiveEventBatchItem,
  LiveExecution,
  LiveExecutionsById,
  StepCompletedEvent,
  StepPausedEvent,
  StepResumedEvent,
  StepStartedEvent,
  StepUpdate,
  ExecutionUpdate,
} from './signalrTypes';
import { parseOutputParametersJson } from '../lib/outputParameters';

export function mergeHydrated(hydrated: LiveExecutionsById, prev: LiveExecutionsById): LiveExecutionsById {
  // REST API snapshot is the baseline; SignalR events already applied to `prev` take precedence
  // for the same stepId (they carry more recent status changes).
  const result: LiveExecutionsById = { ...hydrated };
  for (const [execId, exec] of Object.entries(prev)) {
    if (!result[execId]) {
      result[execId] = exec;
    } else {
      const stepMap = new Map(result[execId].steps.map((s) => [s.stepId, s]));
      for (const step of exec.steps) stepMap.set(step.stepId, step);
      const isTerminal = (s: string) => s === 'Succeeded' || s === 'Failed' || s === 'Cancelled';
      result[execId] = {
        ...result[execId],
        steps: Array.from(stepMap.values()),
        databus: { ...result[execId].databus, ...exec.databus },
        status: isTerminal(exec.status) ? exec.status : result[execId].status,
        completedAt: exec.completedAt ?? result[execId].completedAt,
        errorMessage: exec.errorMessage ?? result[execId].errorMessage,
      };
    }
  }
  return trimLiveExecutions(result);
}

export function isActiveExecution(execution: LiveExecution): boolean {
  return execution.status === 'Running'
    || execution.status === 'Pending'
    || execution.steps.some((s) => s.status === 'Running' || s.status === 'Paused');
}

export function sortLiveExecutions(executions: LiveExecution[]): LiveExecution[] {
  // Pre-compute sort keys so steps.some() runs once per execution instead of
  // O(N log N) times inside the comparator — makes a measurable difference when
  // many executions have steps loaded simultaneously.
  const keyed = executions.map((e) => ({
    exec: e,
    isPaused: e.steps.some((s) => s.status === 'Paused') ? 1 : 0,
    isActive: isActiveExecution(e) ? 1 : 0,
    startedAt: new Date(e.startedAt).getTime(),
  }));
  keyed.sort((a, b) => {
    if (a.isPaused !== b.isPaused) return b.isPaused - a.isPaused;
    if (a.isActive !== b.isActive) return b.isActive - a.isActive;
    return b.startedAt - a.startedAt;
  });
  return keyed.map(({ exec }) => exec);
}

export function trimLiveExecutions(next: LiveExecutionsById): LiveExecutionsById {
  // Active (Running/Pending) runs are never evicted — their ExecutionStatusChanged
  // events must land even when 200+ run concurrently. Each listing-only run costs
  // ~200 bytes; 200 active runs = 40 KB, which is negligible.
  // Only completed runs are capped at 50 (oldest evicted first).
  const all = Object.values(next);
  const completed = all.filter((e) => !isActiveExecution(e));
  if (completed.length <= 50) return next;
  const active = all.filter(isActiveExecution);
  const kept = [...active, ...sortLiveExecutions(completed).slice(0, 50)];
  return Object.fromEntries(kept.map((e) => [e.executionId, e]));
}

/**
 * Merges an execution listing (status-only, no steps) into live state.
 * Used for the initial bulk-load pass: all relevant runs appear immediately as
 * status badges without waiting for N parallel GET /steps fetches.
 * Existing in-memory entries (from SignalR events or prior hydration) are preserved;
 * only status/completedAt/errorMessage are updated from the listing for terminal runs.
 */
export function mergeListingOnly(items: ApiExecutionItem[], prev: LiveExecutionsById): LiveExecutionsById {
  const next = { ...prev };
  const isTerminal = (s: string) => s === 'Succeeded' || s === 'Failed' || s === 'Cancelled';
  for (const e of items) {
    if (!next[e.id]) {
      next[e.id] = {
        executionId: e.id,
        workflowId: e.workflowId,
        status: e.status,
        startedAt: e.startedAt,
        completedAt: e.completedAt,
        errorMessage: e.errorMessage,
        steps: [],
        databus: {},
      };
    } else if (isTerminal(e.status)) {
      next[e.id] = {
        ...next[e.id],
        status: e.status,
        completedAt: e.completedAt,
        errorMessage: e.errorMessage,
      };
    }
  }
  return trimLiveExecutions(next);
}

/**
 * Builds the databus entries for one step in the live view. Single source of truth for
 * the mapping `(stepId, alias?, output, error, paramMap) → DatabusEntry-Dict`, shared
 * between live StepCompleted events ({@link applyLiveEventUpdate}) and post-refresh
 * hydration ({@link buildDatabusFromHydratedSteps}). Matches the engine's
 * BuildStepVariables dual-lookup contract: every entry is written under both
 * `{stepId}.<channel>` and `{alias}.<channel>` keys so a downstream template that
 * references either form resolves identically in the UI overlay.
 */
export function buildStepDatabusEntries(args: {
  stepId: string;
  stepName?: string | null;
  outputVariable?: string | null;
  output?: string | null;
  errorOutput?: string | null;
  outputParameters?: Record<string, string> | null;
}): Record<string, DatabusEntry> {
  const { stepId, stepName, outputVariable, output, errorOutput, outputParameters } = args;
  const out: Record<string, DatabusEntry> = {};
  const heads = [stepId];
  if (outputVariable && outputVariable !== stepId) heads.push(outputVariable);

  for (const head of heads) {
    out[`${head}.output`] = { value: output ?? '', stepId, stepName: stepName ?? undefined, kind: 'output' };
    out[`${head}.error`] = { value: errorOutput ?? '', stepId, stepName: stepName ?? undefined, kind: 'error' };
  }
  if (outputParameters) {
    for (const [k, v] of Object.entries(outputParameters)) {
      for (const head of heads) {
        out[`${head}.param.${k}`] = { value: v, stepId, stepName: stepName ?? undefined, kind: 'param', paramKey: k };
      }
    }
  }
  return out;
}

/**
 * Reconstructs the databus from hydrated REST step rows (`/executions/{id}/steps`). Used
 * on browser refresh or lazy backfill: SignalR didn't deliver these `StepCompleted` events
 * in this session, so the live-event reducer never wrote the entries. Parses
 * `outputParametersJson` (already redacted server-side, may be null/empty for params-free
 * activities like `delay`/`log`) and reuses {@link buildStepDatabusEntries} so the keys
 * are byte-identical to what a live event would have produced.
 */
export function buildDatabusFromHydratedSteps(
  steps: Array<{
    stepId: string;
    stepName?: string | null;
    output?: string | null;
    errorOutput?: string | null;
    outputParametersJson?: string | null;
    outputVariable?: string | null;
  }>,
): Record<string, DatabusEntry> {
  const databus: Record<string, DatabusEntry> = {};
  for (const s of steps) {
    // Best-effort: malformed JSON leaves param entries absent, .output/.error still
    // get reconstructed. Hydration must never throw — a broken row shouldn't blank
    // the whole live view.
    const outputParameters = parseOutputParametersJson(s.outputParametersJson);
    Object.assign(
      databus,
      buildStepDatabusEntries({
        stepId: s.stepId,
        stepName: s.stepName,
        outputVariable: s.outputVariable,
        output: s.output,
        errorOutput: s.errorOutput,
        outputParameters,
      }),
    );
  }
  return databus;
}

export function classifyEntry(key: string, value: string, stepNameMap: Map<string, string | null | undefined>): DatabusEntry {
  // Engine only populates two runtime namespaces — globals.* and manual.*. There is no
  // trigger.* / webhook.* namespace (webhook payload lives under manual.webhook…).
  if (key.startsWith('manual.')) return { value, kind: 'trigger' };
  if (key.startsWith('globals.')) return { value, kind: 'global' };
  const paramM = /^([^.]+)\.param\.(.+)$/.exec(key);
  if (paramM) return { value, stepId: paramM[1], stepName: stepNameMap.get(paramM[1]), kind: 'param', paramKey: paramM[2] };
  const outM = /^([^.]+)\.output$/.exec(key);
  if (outM) return { value, stepId: outM[1], stepName: stepNameMap.get(outM[1]), kind: 'output' };
  const errM = /^([^.]+)\.error$/.exec(key);
  if (errM) return { value, stepId: errM[1], stepName: stepNameMap.get(errM[1]), kind: 'error' };
  return { value, kind: 'other' };
}

/**
 * Returns the single updated exec entry (or null when the event has no effect).
 * Reads the current exec from `state` — the caller's pre-spread working copy —
 * so the function itself never needs to spread the full dict.
 */
export function applyLiveEventUpdate(
  state: LiveExecutionsById,
  event: LiveEvent,
): { id: string; exec: LiveExecution } | null {
  switch (event.type) {
    case 'StepStarted': {
      const { evt } = event;
      const exec = state[evt.executionId]
        ?? { executionId: evt.executionId, workflowId: evt.workflowId, status: 'Running', steps: [], startedAt: evt.startedAt, databus: {} };
      return {
        id: evt.executionId,
        exec: {
          ...exec,
          workflowId: exec.workflowId ?? evt.workflowId,
          status: 'Running',
          steps: [
            ...exec.steps.filter((s) => s.stepId !== evt.stepId),
            { executionId: evt.executionId, workflowId: evt.workflowId, stepId: evt.stepId, stepName: evt.stepName, stepType: evt.stepType, status: 'Running', startedAt: evt.startedAt, traceId: evt.traceId, spanId: evt.spanId },
          ],
        },
      };
    }

    case 'StepCompleted': {
      const { evt } = event;
      const exec = state[evt.executionId]
        ?? { executionId: evt.executionId, workflowId: evt.workflowId, status: evt.status, steps: [], startedAt: evt.completedAt, databus: {} };
      const existingStep = exec.steps.find((s) => s.stepId === evt.stepId);
      const stepName = existingStep?.stepName ?? evt.stepName;
      // The live databus mirrors the engine's variable dictionary: every step that
      // produces output is addressable both by its `{stepId}` and by its configured
      // `outputVariable` alias. Before this fix, only the `.param.*` entries were written
      // here — `.output`/`.error` were missing entirely and the alias was never visible.
      // As a result, the hover preview / expression tester showed less data during a
      // running workflow than the paused debug snapshot did.
      const newEntries = buildStepDatabusEntries({
        stepId: evt.stepId,
        stepName,
        outputVariable: evt.outputVariable,
        output: evt.output,
        errorOutput: evt.errorOutput,
        outputParameters: evt.outputParameters,
      });

      const completedStep: StepUpdate = {
        executionId: evt.executionId,
        workflowId: evt.workflowId,
        stepId: evt.stepId,
        stepName,
        stepType: evt.stepType ?? existingStep?.stepType ?? 'unknown',
        startedAt: existingStep?.startedAt ?? evt.startedAt ?? undefined,
        status: evt.status as StepUpdate['status'],
        output: evt.output,
        errorOutput: evt.errorOutput,
        traceOutput: evt.traceOutput ?? existingStep?.traceOutput,
        completedAt: evt.completedAt,
        traceId: evt.traceId ?? existingStep?.traceId,
        spanId: evt.spanId ?? existingStep?.spanId,
      };

      return {
        id: evt.executionId,
        exec: {
          ...exec,
          workflowId: exec.workflowId ?? evt.workflowId,
          steps: [
            ...exec.steps.filter((s) => s.stepId !== evt.stepId),
            completedStep,
          ],
          databus: { ...exec.databus, ...newEntries },
        },
      };
    }

    case 'ExecutionStatusChanged': {
      const { evt } = event;
      const exec = state[evt.executionId];
      if (!exec) return null;

      // When the execution reaches a terminal state, finalize any steps that are still
      // marked Running. This happens when StepCompleted events were dropped due to
      // SignalR channel overflow (DropNewest) or a brief network hiccup — the step's
      // Completed event never arrived but the execution itself is done. Leaving them
      // as Running would permanently show a spinner in the Live View.
      const isTerminal = evt.status === 'Succeeded' || evt.status === 'Failed' || evt.status === 'Cancelled';
      // Map execution terminal status to a valid StepUpdate status (no 'Cancelled' on step level).
      const terminalStepStatus: StepUpdate['status'] =
        evt.status === 'Succeeded' ? 'Succeeded' :
        evt.status === 'Cancelled' ? 'Skipped' :
        'Failed';
      const finalSteps = isTerminal
        ? exec.steps.map((s) =>
            s.status === 'Running'
              ? { ...s, status: terminalStepStatus, completedAt: evt.completedAt ?? undefined }
              : s
          )
        : exec.steps;

      return {
        id: evt.executionId,
        exec: {
          ...exec,
          workflowId: exec.workflowId ?? evt.workflowId,
          status: evt.status,
          completedAt: evt.completedAt,
          errorMessage: evt.errorMessage,
          steps: finalSteps,
        },
      };
    }

    case 'StepPaused': {
      const { evt } = event;
      const exec = state[evt.executionId]
        ?? { executionId: evt.executionId, workflowId: evt.workflowId, status: 'Running', steps: [], startedAt: evt.pausedAt, databus: {} };
      const existingStep = exec.steps.find((s) => s.stepId === evt.stepId);
      const pausedStep: StepUpdate = {
        executionId: evt.executionId,
        workflowId: evt.workflowId,
        stepId: evt.stepId,
        stepName: existingStep?.stepName ?? evt.stepName,
        stepType: existingStep?.stepType ?? 'unknown',
        startedAt: existingStep?.startedAt,
        status: 'Paused',
        pausedAt: evt.pausedAt,
        pausedVariables: evt.variables,
        pausedReason: evt.reason,
      };
      const steps = [
        ...exec.steps.filter((s) => s.stepId !== evt.stepId),
        pausedStep,
      ];
      const stepNameMap = new Map(steps.map((s) => [s.stepId, s.stepName]));
      const newEntries: Record<string, DatabusEntry> = {};
      for (const [key, value] of Object.entries(evt.variables)) {
        newEntries[key] = classifyEntry(key, value, stepNameMap);
      }
      return {
        id: evt.executionId,
        exec: { ...exec, workflowId: exec.workflowId ?? evt.workflowId, steps, databus: { ...exec.databus, ...newEntries } },
      };
    }

    case 'StepResumed': {
      const { evt } = event;
      const exec = state[evt.executionId];
      if (!exec) return null;
      return {
        id: evt.executionId,
        exec: {
          ...exec,
          workflowId: exec.workflowId ?? evt.workflowId,
          steps: exec.steps.map((s) =>
            s.stepId === evt.stepId
              ? { ...s, status: 'Running', pausedAt: undefined, pausedVariables: undefined, pausedReason: undefined }
              : s,
          ),
        },
      };
    }
  }
}

export function applyLiveEvents(prev: LiveExecutionsById, events: LiveEvent[]): LiveExecutionsById {
  if (events.length === 0) return prev;
  // One shallow copy upfront so all events in the batch share the same baseline.
  // The old pattern spread the full dict inside each applyLiveEvent call, giving
  // O(N × M) property copies for N executions × M events. This is O(N + M).
  const next = { ...prev };
  let mutated = false;
  for (const event of events) {
    const update = applyLiveEventUpdate(next, event);
    if (update !== null) {
      next[update.id] = update.exec;
      mutated = true;
    }
  }
  return mutated ? trimLiveExecutions(next) : prev;
}

export function normalizeBatchItem(item: LiveEventBatchItem): LiveEvent | null {
  const type = item.type ?? item.Type;
  const evt = item.event ?? item.Event ?? item.evt;
  if (!type || !evt) return null;
  switch (type) {
    case 'StepStarted': return { type, evt: evt as StepStartedEvent };
    case 'StepCompleted': return { type, evt: evt as StepCompletedEvent };
    case 'ExecutionStatusChanged': return { type, evt: evt as ExecutionUpdate };
    case 'StepPaused': return { type, evt: evt as StepPausedEvent };
    case 'StepResumed': return { type, evt: evt as StepResumedEvent };
    default: return null;
  }
}
