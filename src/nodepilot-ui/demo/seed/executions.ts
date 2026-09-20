/**
 * Execution history, derived by walking the seed graphs.
 *
 * Nothing here is authored: every run replays a real definition through the same walk the
 * designer preview uses, so a step that appears in the history also exists on the canvas.
 * The dashboard is then derived from these runs, which is what keeps the three screens from
 * contradicting each other.
 */
import type { StepExecution, Workflow, WorkflowExecution } from '../../src/types/api';
import { simulateWorkflow } from '../../src/lib/workflowSimulation';
import { definitionOf, type GraphNode } from '../state/world';
import { demoId } from '../state/ids';
import { failureFor, outcomeFor } from '../run/outcomes';
import { DEMO_USER } from './entities';
import { FILE_DEFAULTS, FILE_FAILURE_INPUTS, FILE_WORKFLOW_ID, fileOutcome, publishFileOutcome } from '../run/fileScenario';

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;

/** Deterministic pseudo-random sequence, so the history is identical on every load. */
function sequence(seed: string): () => number {
  let state = 0;
  for (let i = 0; i < seed.length; i++) state = (Math.imul(state, 31) + seed.charCodeAt(i)) >>> 0;
  return () => {
    state ^= state << 13; state >>>= 0;
    state ^= state >>> 17;
    state ^= state << 5; state >>>= 0;
    return state / 0x1_0000_0000;
  };
}

/**
 * How often each workflow ran, how it is triggered, and how long ago its most recent run was.
 *
 * `phaseHours` staggers the workflows against each other. Without it every workflow's newest
 * run lands a few minutes before now, and the dashboard's hourly chart shows one implausible
 * spike at the right edge instead of a spread.
 */
const CADENCE = [
  // The two smallest phases are deliberate: Live-Ops opens on a 30-minute window, and a seed
  // whose newest run is older than that greets the visitor with an empty console.
  { runs: 48, everyHours: 3, phaseHours: 0.12, triggeredBy: 'schedule' },
  { runs: 30, everyHours: 5, phaseHours: 5.2, triggeredBy: 'manual' },
  { runs: 40, everyHours: 4, phaseHours: 0.34, triggeredBy: 'schedule' },
  // `triggeredBy` matches the trigger the workflow in that slot actually declares, so the
  // executions list does not attribute a scheduled run to a person.
  { runs: 20, everyHours: 8, phaseHours: 9.4, triggeredBy: 'schedule' },
  { runs: 14, everyHours: 12, phaseHours: 14.1, triggeredBy: 'manual' },
];

export interface DerivedHistory {
  executions: WorkflowExecution[];
  steps: Map<string, StepExecution[]>;
}

function stepRowsFor(
  executionId: string,
  order: string[],
  nodeById: Map<string, GraphNode>,
  startedAtMs: number,
  failAtIndex: number | null,
  cancelAtIndex: number | null,
  fileInputs?: Record<string, string>,
): { rows: StepExecution[]; endedAtMs: number } {
  const rows: StepExecution[] = [];
  const bus: Record<string, string> = {};
  let cursor = startedAtMs;

  for (let i = 0; i < order.length; i++) {
    const node = nodeById.get(order[i]);
    const activityType = node?.data?.activityType;
    const outcome = fileInputs && node ? fileOutcome(node, fileInputs, bus) ?? outcomeFor(activityType) : outcomeFor(activityType);
    const scenarioError = 'error' in outcome ? outcome.error as string | undefined : undefined;
    const stepStarted = cursor;
    const stepEnded = cursor + outcome.durationMs;
    cursor = stepEnded;

    const failed = failAtIndex === i || Boolean(scenarioError);
    const cancelled = cancelAtIndex === i;
    rows.push({
      id: demoId(`step:${executionId}:${order[i]}`),
      stepId: order[i],
      stepName: node?.data?.label ?? null,
      stepType: activityType ?? 'unknown',
      targetMachine: node?.data?.targetMachineId ?? null,
      status: failed ? 'Failed' : cancelled ? 'Cancelled' : 'Succeeded',
      startedAt: new Date(stepStarted).toISOString(),
      completedAt: new Date(stepEnded).toISOString(),
      output: failed || cancelled ? null : outcome.output,
      errorOutput: failed ? scenarioError ?? failureFor(activityType) : null,
      attemptCount: 1,
      traceOutput: null,
      outputParametersJson:
        failed || cancelled || Object.keys(outcome.outputParameters).length === 0
          ? null
          : JSON.stringify(outcome.outputParameters),
      outputVariable: node?.data?.outputVariable ?? null,
    });
    if (!failed && !cancelled && node) publishFileOutcome(bus, node, outcome);

    if (failed || cancelled) break;
  }

  return { rows, endedAtMs: cursor };
}

/** Builds the full history for every seed workflow. */
export function buildHistory(workflows: Workflow[], now: number): DerivedHistory {
  const executions: WorkflowExecution[] = [];
  const steps = new Map<string, StepExecution[]>();

  workflows.forEach((workflow, workflowIndex) => {
    const guided = workflow.id === FILE_WORKFLOW_ID;
    const cadence = guided ? { runs: 2, everyHours: 24, phaseHours: 2, triggeredBy: 'manual' } : CADENCE[workflowIndex % CADENCE.length];
    const { nodes, edges } = definitionOf(workflow);
    const nodeById = new Map(nodes.map((n) => [n.id, n]));
    const order = simulateWorkflow(nodes, edges).order;
    if (order.length === 0) return;

    const next = sequence(workflow.id);

    for (let run = 0; run < cadence.runs; run++) {
      const executionId = demoId(`execution:${workflow.id}:${run}`);
      // Newest run first: run 0 is the most recent, offset by this workflow's phase.
      //
      // Jitter is applied from the second run onwards only. It spans up to 40 minutes, which
      // is wider than the smallest phases and would push the newest run back out of the
      // 30-minute window Live-Ops opens on — the console would greet a visitor empty.
      const jitterMs = run === 0 ? 0 : Math.floor(next() * 40 * MINUTE);
      const startedAtMs =
        now - cadence.phaseHours * HOUR - run * cadence.everyHours * HOUR - jitterMs;

      // A fleet that mostly works, and that has been getting better: recent runs fail less than
      // older ones. That keeps the 24-hour window — the first thing a visitor sees — healthy
      // without flattening the longer views to a staged 100 %, and it is the shape a maintained
      // installation actually has.
      const roll = next();
      const recent = run < 8;
      const failureRate = recent ? 0.02 : 0.048;
      const isFailed = guided ? run === 1 : roll < failureRate;
      // Cancellations are an operator action, and the demo's recent window should read as a
      // quiet week; older runs still carry a few so the status filter has something to show.
      const isCancelled = !guided && !isFailed && !recent && roll < failureRate + 0.02;
      const fileInputs = guided ? (isFailed ? FILE_FAILURE_INPUTS : FILE_DEFAULTS) : undefined;
      const breakIndex = order.length > 2 ? 1 + Math.floor(next() * (order.length - 2)) : order.length - 1;

      const { rows, endedAtMs } = stepRowsFor(
        executionId,
        order,
        nodeById,
        startedAtMs,
        isFailed && !guided ? breakIndex : null,
        isCancelled ? breakIndex : null,
        fileInputs,
      );
      steps.set(executionId, rows);

      const failedRows = rows.filter((r) => r.status === 'Failed');
      executions.push({
        id: executionId,
        workflowId: workflow.id,
        status: isFailed ? 'Failed' : isCancelled ? 'Cancelled' : 'Succeeded',
        startedAt: new Date(startedAtMs).toISOString(),
        completedAt: new Date(endedAtMs).toISOString(),
        triggeredBy: cadence.triggeredBy,
        errorMessage: isFailed ? failedRows[0]?.errorOutput ?? null : null,
        traceId: null,
        spanId: null,
        returnData: guided && !isFailed ? rows.find(row => row.stepType === 'returnData')?.outputParametersJson ?? null : null,
        inputParametersJson: fileInputs ? JSON.stringify(fileInputs) : null,
        startedByUsername: cadence.triggeredBy === 'manual' ? DEMO_USER.username : null,
        parentExecutionId: null,
        parentWorkflowName: null,
        stepsTotal: order.length,
        stepsCompleted: rows.filter((r) => r.status === 'Succeeded').length,
        failedSteps: failedRows.map((r) => ({ stepId: r.stepId, stepName: r.stepName })),
      });
    }
  });

  executions.sort((a, b) => Date.parse(b.startedAt) - Date.parse(a.startedAt));
  return { executions, steps };
}
