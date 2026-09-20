/**
 * Plays a workflow run in the browser.
 *
 * It walks whatever graph is on the canvas right now, not a recorded script: a visitor who
 * adds, renames or deletes nodes still gets a run that lights up their own graph, and a
 * workflow built from scratch behaves like a seeded one.
 *
 * Every step is written into the world **before** its event is delivered. The operations feed
 * debounce-invalidates query keys on each event, and the refetch that follows hits the demo's
 * own REST layer — emitting first would serve a state older than the event that triggered it
 * and make the UI flicker backwards.
 */
import type { StepExecution, WorkflowExecution } from '../../src/types/api';
import { simulateWorkflow } from '../../src/lib/workflowSimulation';
import { definitionOf, findWorkflow, getWorld, notifyWorld, type GraphNode } from '../state/world';
import { runtimeId } from '../state/ids';
import { emitExecutionStatus, emitStepEvent } from '../hub/fakeHub';
import { outcomeFor } from './outcomes';
import { FILE_DEFAULTS, FILE_WORKFLOW_ID, fileOutcome, publishFileOutcome } from './fileScenario';

/** Real step durations would make a run take a minute; the demo compresses them. */
const MIN_STEP_MS = 320;
const MAX_STEP_MS = 1_400;

function pacedDuration(realMs: number): number {
  return Math.round(Math.min(MAX_STEP_MS, Math.max(MIN_STEP_MS, realMs / 4)));
}

interface ActiveRun {
  executionId: string;
  timer: ReturnType<typeof setTimeout> | null;
  cancelled: boolean;
}

const active = new Map<string, ActiveRun>();

function finalize(run: ActiveRun, status: string, errorMessage: string | null): void {
  const world = getWorld();
  const execution = world.executions.find((e) => e.id === run.executionId);
  if (!execution) return;

  const rows = world.steps.get(run.executionId) ?? [];
  execution.status = status;
  execution.completedAt = new Date().toISOString();
  execution.errorMessage = errorMessage;
  execution.stepsCompleted = rows.filter((r) => r.status === 'Succeeded').length;
  execution.failedSteps = rows
    .filter((r) => r.status === 'Failed')
    .map((r) => ({ stepId: r.stepId, stepName: r.stepName }));

  // Re-derive the workflow's own counters from the history. Without this the list still
  // shows the seeded run count while the history tab already shows one more.
  const workflow = world.workflows.find((w) => w.id === execution.workflowId);
  if (workflow) {
    const runs = world.executions.filter((e) => e.workflowId === workflow.id);
    const durations = runs
      .filter((e) => e.completedAt)
      .map((e) => Date.parse(e.completedAt!) - Date.parse(e.startedAt));
    workflow.totalCount = runs.length;
    workflow.successCount = runs.filter((e) => e.status === 'Succeeded').length;
    workflow.avgDurationMs = durations.length === 0
      ? null
      : Math.round(durations.reduce((a, b) => a + b, 0) / durations.length);
    workflow.lastExecution = {
      id: execution.id,
      status,
      startedAt: execution.startedAt,
      completedAt: execution.completedAt,
      durationMs: Date.parse(execution.completedAt) - Date.parse(execution.startedAt),
    };
  }

  active.delete(run.executionId);
  notifyWorld();
  emitExecutionStatus({
    executionId: execution.id,
    workflowId: execution.workflowId,
    status,
    errorMessage,
    completedAt: execution.completedAt,
  });
}

/**
 * Starts a run of `workflowId`. Returns the execution row immediately, the way
 * `POST /execute` does — the UI navigates on the id and then follows the hub.
 */
export function startRun(workflowId: string, triggeredBy = 'manual', parameters: Record<string, string> = {}): WorkflowExecution | null {
  const workflow = findWorkflow(workflowId);
  if (!workflow) return null;

  const world = getWorld();
  const { nodes, edges } = definitionOf(workflow);
  const nodeById = new Map(nodes.map((n) => [n.id, n]));
  const order = simulateWorkflow(nodes, edges).order;
  const inputs = workflowId === FILE_WORKFLOW_ID ? { ...FILE_DEFAULTS, ...parameters } : parameters;
  const bus: Record<string, string> = {};

  const executionId = runtimeId('execution');
  const startedAt = new Date().toISOString();
  const execution: WorkflowExecution = {
    id: executionId,
    workflowId,
    status: 'Running',
    startedAt,
    completedAt: null,
    triggeredBy,
    errorMessage: null,
    traceId: null,
    spanId: null,
    returnData: null,
    inputParametersJson: Object.keys(inputs).length ? JSON.stringify(inputs) : null,
    startedByUsername: null,
    parentExecutionId: null,
    parentWorkflowName: null,
    stepsTotal: order.length,
    stepsCompleted: 0,
    failedSteps: [],
  };

  world.executions.unshift(execution);
  world.steps.set(executionId, []);

  const run: ActiveRun = { executionId, timer: null, cancelled: false };
  active.set(executionId, run);
  notifyWorld();

  emitExecutionStatus({ executionId, workflowId, status: 'Running' });

  // A graph with no enabled trigger reaches nothing — the engine fails the run rather than
  // succeeding with zero steps, and so does the demo.
  if (order.length === 0) {
    run.timer = globalThis.setTimeout(() => {
      finalize(run, 'Failed', 'The workflow has no enabled trigger, so it has no entry point.');
    }, 400);
    return execution;
  }

  let index = 0;

  const startStep = () => {
    if (run.cancelled) return;
    const node: GraphNode | undefined = nodeById.get(order[index]);
    const activityType = node?.data?.activityType;
    const outcome = workflowId === FILE_WORKFLOW_ID && node ? fileOutcome(node, inputs, bus) ?? outcomeFor(activityType) : outcomeFor(activityType);
    const error = 'error' in outcome ? outcome.error as string | undefined : undefined;
    const stepStartedAt = new Date().toISOString();

    const row: StepExecution = {
      id: runtimeId('step'),
      stepId: order[index],
      stepName: node?.data?.label ?? null,
      stepType: activityType ?? 'unknown',
      targetMachine: node?.data?.targetMachineId ?? null,
      status: 'Running',
      startedAt: stepStartedAt,
      completedAt: null,
      output: null,
      errorOutput: null,
      attemptCount: 1,
      traceOutput: null,
      outputParametersJson: null,
      outputVariable: node?.data?.outputVariable ?? null,
    };
    // World first, event second.
    getWorld().steps.get(run.executionId)?.push(row);
    emitStepEvent({
      name: 'StepStarted',
      event: {
        executionId: run.executionId,
        workflowId,
        stepId: row.stepId,
        stepName: row.stepName ?? undefined,
        stepType: row.stepType,
        startedAt: stepStartedAt,
      },
    });

    run.timer = globalThis.setTimeout(() => {
      if (!error && node) publishFileOutcome(bus, node, outcome);
      completeStep(row, outcome.output, outcome.outputParameters, error);
    }, pacedDuration(outcome.durationMs));
  };

  const completeStep = (row: StepExecution, output: string, params: Record<string, string>, error?: string) => {
    if (run.cancelled) return;
    const completedAt = new Date().toISOString();
    row.status = error ? 'Failed' : 'Succeeded';
    row.completedAt = completedAt;
    row.output = output;
    row.errorOutput = error ?? null;
    row.outputParametersJson = Object.keys(params).length > 0 ? JSON.stringify(params) : null;

    const current = getWorld().executions.find((e) => e.id === run.executionId);
    if (current && !error) {
      current.stepsCompleted = (current.stepsCompleted ?? 0) + 1;
      if (row.stepType === 'returnData') current.returnData = JSON.stringify(params);
    }
    notifyWorld();

    emitStepEvent({
      name: 'StepCompleted',
      event: {
        executionId: run.executionId,
        workflowId,
        stepId: row.stepId,
        stepName: row.stepName,
        status: row.status,
        errorOutput: error,
        output,
        completedAt,
        outputParameters: Object.keys(params).length > 0 ? params : null,
        stepType: row.stepType,
        startedAt: row.startedAt,
        outputVariable: row.outputVariable,
      },
    });

    if (error) { finalize(run, 'Failed', error); return; }

    index += 1;
    if (index >= order.length) {
      run.timer = globalThis.setTimeout(() => finalize(run, 'Succeeded', null), 260);
      return;
    }
    run.timer = globalThis.setTimeout(startStep, 160);
  };

  run.timer = globalThis.setTimeout(startStep, 320);
  return execution;
}

/** Cancels a running demo execution, leaving the current step Cancelled. */
export function cancelRun(executionId: string): boolean {
  const run = active.get(executionId);
  if (!run) return false;
  run.cancelled = true;
  if (run.timer !== null) globalThis.clearTimeout(run.timer);

  const world = getWorld();
  for (const row of world.steps.get(executionId) ?? []) {
    if (row.status === 'Running') {
      row.status = 'Cancelled';
      row.completedAt = new Date().toISOString();
    }
  }
  finalize(run, 'Cancelled', null);
  return true;
}

/** Stops every in-flight run. Used on world reset. */
export function stopAllRuns(): void {
  for (const run of [...active.values()]) {
    run.cancelled = true;
    if (run.timer !== null) globalThis.clearTimeout(run.timer);
    active.delete(run.executionId);
  }
}

/** Execution ids currently playing, for tests. */
export function activeRunIds(): string[] {
  return [...active.keys()];
}
